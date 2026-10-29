using System.Text.Json.Nodes;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Writes lifecycle events in the order required by the RPC stream.</summary>
internal sealed class RpcEventWriter(JsonLineWriter output, Func<JsonObject?>? getSystemMessage = null)
{
    private readonly object _projectorGate = new();
    private readonly object _retryEndGate = new();
    private readonly List<(long AfterVersion, TaskCompletionSource Completion)> _retryEndWaiters = [];
    private RpcEventProjector? _activeProjector;
    private string? _activeApi;
    private JsonObject? _lastSystemMessage;
    private long _retryEndVersion;

    public long RetryEndVersion
    {
        get { lock (_retryEndGate) return _retryEndVersion; }
    }

    public Task WaitForRetryEndAfterAsync(long version)
    {
        lock (_retryEndGate)
        {
            if (_retryEndVersion > version) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _retryEndWaiters.Add((version, completion));
            return completion.Task;
        }
    }

    public JsonArray ProjectMessagesSnapshot(ConversationSession conversation, JsonObject? systemMessage = null)
    {
        lock (_projectorGate)
            return (_activeProjector ?? new RpcEventProjector()).ProjectMessagesSnapshot(conversation, _activeApi, systemMessage);
    }

    public string? GetLastAssistantTextSnapshot(ConversationSession conversation)
    {
        lock (_projectorGate)
            return (_activeProjector ?? new RpcEventProjector()).GetLastAssistantTextSnapshot(conversation, _activeApi);
    }

    public Task EmitThinkingLevelChangedAsync(string level, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "thinking_level_changed", level }, cancellationToken);

    public Task EmitSessionInfoChangedAsync(string? name, CancellationToken cancellationToken = default) =>
        output.EmitAsync(new { type = "session_info_changed", name }, cancellationToken);

    public async Task EmitCommandLifecycleAsync(ConversationRun run, AgentLifecycleEvent item)
    {
        var projector = new RpcEventProjector();
        foreach (var record in projector.Project(item, run.Conversation, null, null, false, null))
            await output.EmitAsync(record, CancellationToken.None);
        if (item.Type == "auto_retry_end") SignalRetryEndEmitted();
    }

    public Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? observeEvent = null, string? api = null) =>
        RunEventsAsync(run, run.RunEventsAsync(prompt, cancellationToken), cancellationToken, observeEvent, api);

    public async Task<bool> RunEventsAsync(ConversationRun run, IAsyncEnumerable<AgentLifecycleEvent> events,
        CancellationToken cancellationToken = default, Func<AgentLifecycleEvent, Task>? observeEvent = null,
        string? api = null)
    {
        var projector = new RpcEventProjector();
        projector.SetThinkingLevel(run.ThinkingLevel);
        var pendingPromptMessages = new List<AgentLifecycleEvent>();
        lock (_projectorGate)
        {
            _activeProjector = projector;
            _activeApi = api;
            projector.SetSystemMessage(_lastSystemMessage);
        }
        var succeeded = false;
        var accepted = false;
        var turnOpen = false;
        string? runStartHead = null;
        string? turnStartHead = null;
        try
        {
            await foreach (var item in events)
            {
                projector.SetThinkingLevel(run.ThinkingLevel);
                if (observeEvent is not null) await observeEvent(item);
                if (item.Type == "prompt_accepted")
                {
                    if (!accepted)
                    {
                        runStartHead = item.RunStartHead;
                        await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                        lock (_projectorGate) projector.BeginAgent();
                    }
                    accepted = true;
                    turnStartHead = item.RunStartHead;
                    if (!turnOpen)
                    {
                        await output.EmitAsync(new { type = "turn_start" }, CancellationToken.None);
                        turnOpen = true;
                        lock (_projectorGate) projector.BeginTurn();
                    }
                    pendingPromptMessages.Add(item);
                    continue;
                }
                if (item.Type == "steering_message_accepted")
                {
                    IReadOnlyList<object> promptRecords;
                    lock (_projectorGate) promptRecords = projector.ProjectPrompt(item, run.Conversation);
                    foreach (var record in promptRecords)
                        await output.EmitAsync(record, CancellationToken.None);
                    continue;
                }
                if (item.Type == "agent_attempt_started")
                {
                    runStartHead = item.RunStartHead;
                    turnStartHead = item.RunStartHead;
                    await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                    await output.EmitAsync(new { type = "turn_start" }, CancellationToken.None);
                    lock (_projectorGate) projector.BeginAgent();
                    turnOpen = true;
                    continue;
                }
                if (item.Type == "prompt_rejected") continue;
                if (item.Type == "model_request_started" ||
                    pendingPromptMessages.Count > 0 && item.Type is "turn_completed" or "turn_failed" or "turn_interrupted")
                {
                    JsonObject? currentSystemMessage;
                    lock (_projectorGate) currentSystemMessage = getSystemMessage?.Invoke();
                    if (currentSystemMessage is not null &&
                        _lastSystemMessage?.ToJsonString() != currentSystemMessage.ToJsonString())
                    {
                        IReadOnlyList<object> systemRecords;
                        lock (_projectorGate) systemRecords = projector.ProjectSystemMessage(currentSystemMessage);
                        foreach (var record in systemRecords)
                            await output.EmitAsync(record, CancellationToken.None);
                        lock (_projectorGate) _lastSystemMessage = currentSystemMessage.DeepClone().AsObject();
                    }
                    foreach (var pendingPrompt in pendingPromptMessages)
                    {
                        IReadOnlyList<object> promptRecords;
                        lock (_projectorGate) promptRecords = projector.ProjectPrompt(pendingPrompt, run.Conversation);
                        foreach (var record in promptRecords)
                            await output.EmitAsync(record, CancellationToken.None);
                    }
                    pendingPromptMessages.Clear();
                }
                if (item.Type == "agent_settled")
                {
                    if (accepted) await output.EmitAsync(new { type = "agent_settled", aborted = item.Aborted ?? false },
                        CancellationToken.None);
                    continue;
                }
                if (item.Type == "assistant_turn_started" && turnOpen) continue;
                IReadOnlyList<object> records;
                lock (_projectorGate)
                    records = projector.Project(item, run.Conversation, turnStartHead, runStartHead,
                        accepted, api, turnOpen);
                foreach (var record in records)
                    await output.EmitAsync(record, CancellationToken.None);
                if (item.Type == "auto_retry_end") SignalRetryEndEmitted();
                if (item.Type == "assistant_turn_started") turnOpen = true;
                if (item.Type is "assistant_turn_completed" or "turn_failed" or "turn_interrupted") turnOpen = false;
                if (item.Type == "agent_run_completed") succeeded = true;
            }
        }
        finally
        {
            lock (_projectorGate)
            {
                if (ReferenceEquals(_activeProjector, projector))
                {
                    _activeProjector = null;
                    _activeApi = null;
                }
            }
        }
        return succeeded;
    }

    private void SignalRetryEndEmitted()
    {
        TaskCompletionSource[] ready;
        lock (_retryEndGate)
        {
            _retryEndVersion++;
            ready = _retryEndWaiters.Where(waiter => _retryEndVersion > waiter.AfterVersion)
                .Select(waiter => waiter.Completion).ToArray();
            _retryEndWaiters.RemoveAll(waiter => _retryEndVersion > waiter.AfterVersion);
        }
        foreach (var completion in ready) completion.TrySetResult();
    }
}
