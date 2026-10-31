using System.Text.Json.Nodes;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Writes lifecycle events in the order required by the RPC stream.</summary>
internal sealed class RpcEventWriter(JsonLineWriter output, Func<JsonObject?>? getSystemMessage = null,
    Func<JsonObject?>? getPersistedSystemMessage = null)
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
        // Drain snapshots that belong to a pending batch prompt, kept in drain order so each one is
        // emitted immediately before the message it reflects (pinned Pi takes, publishes, then
        // delivers one queued message at a time).
        var pendingQueueUpdates = new List<AgentLifecycleEvent>();
        lock (_projectorGate)
        {
            _activeProjector = projector;
            _activeApi = api;
            projector.SetSystemMessage(_lastSystemMessage);
        }
        var succeeded = false;
        var accepted = false;
        var agentOpen = false;
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
                    // An accepted prompt starts a new agent run whenever no agent run is open: the
                    // first prompt, and also a queued steering/follow-up batch delivered after a
                    // failed or completed turn (pinned Pi emits agent_start for that continuation).
                    if (!agentOpen)
                    {
                        runStartHead = item.RunStartHead;
                        await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                        agentOpen = true;
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
                    agentOpen = true;
                    lock (_projectorGate) projector.BeginAgent();
                    turnOpen = true;
                    continue;
                }
                if (item.Type == "prompt_rejected") continue;
                if (item.Type == "queue_update" && pendingPromptMessages.Count > 0)
                {
                    pendingQueueUpdates.Add(item);
                    continue;
                }
                if (item.Type == "model_request_started" ||
                    pendingPromptMessages.Count > 0 && item.Type is "turn_completed" or "turn_failed" or "turn_interrupted")
                {
                    JsonObject? currentSystemMessage;
                    lock (_projectorGate) currentSystemMessage = getSystemMessage?.Invoke();
                    // Seed from the restored session so a resumed conversation reports no system
                    // message when nothing changed, and reports one when a section or the tool
                    // loadout did change while the session was closed.
                    _lastSystemMessage ??= getPersistedSystemMessage?.Invoke();
                    if (currentSystemMessage is not null &&
                        !EquivalentSystemMessage(_lastSystemMessage, currentSystemMessage))
                    {
                        IReadOnlyList<object> systemRecords;
                        lock (_projectorGate) systemRecords = projector.ProjectSystemMessage(currentSystemMessage);
                        foreach (var record in systemRecords)
                            await output.EmitAsync(record, CancellationToken.None);
                        lock (_projectorGate) _lastSystemMessage = currentSystemMessage.DeepClone().AsObject();
                    }
                    for (var index = 0; index < pendingPromptMessages.Count; index++)
                    {
                        if (index < pendingQueueUpdates.Count)
                            await EmitBufferedQueueUpdateAsync(pendingQueueUpdates[index]);
                        IReadOnlyList<object> promptRecords;
                        lock (_projectorGate) promptRecords = projector.ProjectPrompt(
                            pendingPromptMessages[index], run.Conversation);
                        foreach (var record in promptRecords)
                            await output.EmitAsync(record, CancellationToken.None);
                    }
                    for (var index = pendingPromptMessages.Count; index < pendingQueueUpdates.Count; index++)
                        await EmitBufferedQueueUpdateAsync(pendingQueueUpdates[index]);
                    pendingPromptMessages.Clear();
                    pendingQueueUpdates.Clear();
                }
                if (item.Type == "agent_settled")
                {
                    // A drain snapshot that never found its prompt must still be reported.
                    foreach (var buffered in pendingQueueUpdates)
                        await EmitBufferedQueueUpdateAsync(buffered);
                    pendingQueueUpdates.Clear();
                    if (accepted) await output.EmitAsync(new { type = "agent_settled", aborted = item.Aborted ?? false },
                        CancellationToken.None);
                    continue;
                }
                if (item.Type == "assistant_turn_started" && !agentOpen)
                {
                    // A continuation batch delivered after a failed or completed turn opens a new
                    // turn without a fresh prompt_accepted; pinned Pi starts a new agent run for it.
                    await output.EmitAsync(new { type = "agent_start" }, CancellationToken.None);
                    agentOpen = true;
                    lock (_projectorGate) projector.BeginAgent();
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
                // These close the agent run: the projector emits agent_end for them.
                if (item.Type is "turn_failed" or "turn_interrupted" or "agent_run_completed") agentOpen = false;
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

        async Task EmitBufferedQueueUpdateAsync(AgentLifecycleEvent queueEvent)
        {
            IReadOnlyList<object> queueRecords;
            lock (_projectorGate)
                queueRecords = projector.Project(queueEvent, run.Conversation, turnStartHead, runStartHead,
                    accepted, api, turnOpen);
            foreach (var record in queueRecords)
                await output.EmitAsync(record, CancellationToken.None);
        }
    }

    /// <summary>
    /// Compares the semantic system state: the model-facing prompt text and the effective tool
    /// declarations, ignoring the timestamp and ignoring whether the prompt is laid out as text or
    /// as sections. A stamp-only change is not a change the model needs to be told about.
    /// </summary>
    private static bool EquivalentSystemMessage(JsonObject? left, JsonObject? right)
    {
        if (left is null || right is null) return false;
        return Canonical(left) == Canonical(right);

        static string Canonical(JsonObject message)
        {
            var parts = new List<string>();
            if (message["content"]?.GetValue<string>() is { Length: > 0 } content) parts.Add(content);
            if (message["sections"] is JsonObject sections)
                parts.AddRange(sections.Select(section => section.Value?.GetValue<string>() ?? "")
                    .Where(text => text.Length > 0));
            // A tool's model-facing identity is its name, description and schema; the display label
            // is presentation only, and older revisions do not carry it.
            var tools = message["toolsAdded"] is JsonArray declarations
                ? declarations.OfType<JsonObject>().Select(tool =>
                {
                    var modelFacing = new JsonObject();
                    foreach (var name in new[] { "name", "description", "parameters" })
                        if (tool[name] is { } value) modelFacing[name] = value.DeepClone();
                    return modelFacing.ToJsonString();
                }).ToArray()
                : [];
            return string.Join("\n\n", parts) + "\u0000" + string.Join("\u0000", tools);
        }
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
